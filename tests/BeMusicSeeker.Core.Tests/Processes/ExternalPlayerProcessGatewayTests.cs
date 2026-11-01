using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using Ribbit.Windows;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed partial class ExternalPlayerProcessGatewayTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();



    /// <summary>空白付き・大小文字重複のprocess名を正規化して重複除去し、探索先を二重に扱いません。</summary>
    [TestMethod]
    public void DiscoveryRequestNormalizesAndCopiesProcessNames()
    {
        var request = ExternalPlayerProcessDiscoveryRequest.Create(
            " LR2body ",
            "LRHbody",
            "lr2body");

        CollectionAssert.AreEqual(
            new[] { "LR2body", "LRHbody" },
            request.ProcessNames.ToArray());
    }

    /// <summary>空白・未指定のprocess探索名と実行pathを拒否し、対象を特定できないprocess操作を作りません。</summary>
    [TestMethod]
    public void RequestFactoriesRejectMissingValues()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ExternalPlayerProcessDiscoveryRequest.Create(" "));
        Assert.ThrowsExactly<ArgumentException>(() => ExternalPlayerProcessDiscoveryRequest.Create());
        Assert.ThrowsExactly<ArgumentException>(() => ExternalPlayerProcessLaunchRequest.Create(" ", "args"));
    }



    /// <summary>LR2の実プレイヤー入口から探索名・譜面引数・非表示起動をgatewayへ渡し、window未準備の失敗を返します。</summary>
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
                "<config><system><windowsize_x>800</windowsize_x><windowsize_y>600</windowsize_y><screenmode>1</screenmode></system><sound><volumemaster>100</volumemaster></sound><jukebox /></config>");
            var gateway = new RecordingExternalPlayerProcessGateway();
            var player = new LR2body(
                executablePath,
                new Lr2ConfigOwner(new LR2Config(configPath)),
                new SettingsPlayerSettingsGateway(() => testSettings),
                gateway,
                waitPolicy: CreateWaitPolicy());
            ((IExternalWindowPlayer)player).AttachWindowHost(new Win32ExternalPlayerWindowHost(IntPtr.Zero));

            Assert.ThrowsExactly<TimeoutException>(() => player.PlayStart(chartPath, (EventHandler)null!));

            Assert.AreEqual(2, gateway.DiscoveryRequest.ProcessNames.Count);
            Assert.AreEqual("LR2body", gateway.DiscoveryRequest.ProcessNames[0]);
            Assert.AreEqual("LRHbody", gateway.DiscoveryRequest.ProcessNames[1]);
            Assert.AreEqual(executablePath, gateway.LaunchRequest.ExecutablePath);
            Assert.AreEqual("-A -NS \"" + chartPath + "\"", gateway.LaunchRequest.Arguments);
            Assert.AreEqual(ProcessWindowStyle.Hidden, gateway.LaunchRequest.WindowStyle);
            Assert.IsTrue(gateway.Session.Started);
        });
    }

    /// <summary>BMIIDXの実入口から両探索名・譜面引数・最小化起動をgatewayへ渡し、processの早期終了を成功扱いしません。</summary>
    [TestMethod]
    public void BmiIdxViewIssuesDiscoveryAndLaunchRequestsThroughGateway()
    {
        WithTemporaryPlayerFiles("BMIIDXView2015_64.exe", (root, executablePath, chartPath) =>
        {
            var gateway = new RecordingExternalPlayerProcessGateway();
            var player = new BMIIDXView2015(
                executablePath,
                new SettingsPlayerSettingsGateway(() => testSettings),
                gateway,
                waitPolicy: CreateWaitPolicy());
            ((IExternalWindowPlayer)player).AttachWindowHost(new Win32ExternalPlayerWindowHost(IntPtr.Zero));

            Assert.ThrowsExactly<InvalidOperationException>(() => player.PlayStart(chartPath, (EventHandler)null!));

            Assert.AreEqual(2, gateway.DiscoveryRequest.ProcessNames.Count);
            Assert.AreEqual("BMIIDXView2015", gateway.DiscoveryRequest.ProcessNames[0]);
            Assert.AreEqual("BMIIDXView2015_64", gateway.DiscoveryRequest.ProcessNames[1]);
            Assert.AreEqual(executablePath, gateway.LaunchRequest.ExecutablePath);
            Assert.AreEqual("-S \"" + chartPath + "\"", gateway.LaunchRequest.Arguments);
            Assert.AreEqual(ProcessWindowStyle.Minimized, gateway.LaunchRequest.WindowStyle);
            Assert.IsTrue(gateway.Session.Started);
        });
    }

    /// <summary>BMIIDX終了通知を供給したhandlerへ一回渡し、sessionとEventArgsを別の値に置換しません。</summary>
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
                new SettingsPlayerSettingsGateway(() => testSettings),
                gateway,
                waitPolicy: CreateWaitPolicy());
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

    /// <summary>完了も中止もない待機は設定期限で元のTimeout診断を返し、無期限に停止しません。</summary>
    [TestMethod]
    public void ExternalPlayerWaitPolicyFailsInsteadOfWaitingIndefinitely()
    {
        var clock = new ManualExternalPlayerTime();
        var policy = new ExternalPlayerWaitPolicy(TimeSpan.FromMilliseconds(25), clock, clock.Wait);

        TimeoutException exception = Assert.ThrowsExactly<TimeoutException>(
            () => policy.WaitUntil(
                completed: () => false,
                aborted: () => false,
                attempt: () => { },
                timeoutMessage: "bounded wait expired",
                pollMilliseconds: 1));

        Assert.AreEqual("bounded wait expired", exception.Message);
        Assert.AreEqual(TimeSpan.FromMilliseconds(25), clock.Elapsed);
    }

    /// <summary>終了待機は期限の半分で一回だけkillを試し、実終了だけを成功にして未終了・kill例外では期限の失敗を返します。</summary>
    [TestMethod]
    [DataRow(true, false)] // killで終了を確認できる。
    [DataRow(false, false)] // kill後も終了しない。
    [DataRow(false, true)] // kill自体が失敗する。
    public void ExternalPlayerExitWaitForcesOnceAtHalfDeadline(bool exitsOnKill, bool killThrows)
    {
        var clock = new ManualExternalPlayerTime();
        var policy = new ExternalPlayerWaitPolicy(TimeSpan.FromMilliseconds(100), clock, clock.Wait);
        bool exited = false;
        int kills = 0;
        TimeSpan? killedAt = null;
        void WaitForExit() => policy.WaitForProcessExit(
            () => exited,
            () =>
            {
                kills++;
                killedAt = clock.Elapsed;
                if (killThrows) { throw new InvalidOperationException("kill failed"); }
                exited = exitsOnKill;
            },
            "exit wait expired");

        if (exitsOnKill) { WaitForExit(); }
        else
        {
            TimeoutException failure = Assert.ThrowsExactly<TimeoutException>(WaitForExit);
            Assert.AreEqual("exit wait expired", failure.Message);
        }

        Assert.AreEqual(1, kills);
        Assert.AreEqual(TimeSpan.FromMilliseconds(50), killedAt);
        Assert.AreEqual(TimeSpan.FromMilliseconds(exitsOnKill ? 50 : 100), clock.Elapsed);
        Assert.AreEqual(exitsOnKill, exited);
    }

    /// <summary>待機途中の中止を次の試行より前に返し、中止を成功や期限超過へ置換しません。</summary>
    [TestMethod]
    public void ExternalPlayerWaitReturnsAbortWithoutAnotherAttempt()
    {
        var clock = new ManualExternalPlayerTime();
        var policy = new ExternalPlayerWaitPolicy(TimeSpan.FromMilliseconds(100), clock, clock.Wait);
        int attempts = 0;

        bool completed = policy.WaitUntil(
            () => false,
            () => attempts == 1,
            () => attempts++,
            "wait expired",
            pollMilliseconds: 10);

        Assert.IsFalse(completed);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), clock.Elapsed);
    }

    /// <summary>BMIIDXのwindow準備が期限超過したら起動processへ閉鎖とkillを一回行い、所有processを生存させません。</summary>
    [TestMethod]
    public void BmiIdxMainWindowTimeoutCleansUpStartedProcess()
    {
        WithTemporaryPlayerFiles("BMIIDXView2015_64.exe", (root, executablePath, chartPath) =>
        {
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            var player = new BMIIDXView2015(
                executablePath,
                new SettingsPlayerSettingsGateway(() => testSettings),
                gateway,
                CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(
                new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99))));

            Assert.ThrowsExactly<TimeoutException>(
                () => player.PlayStart(chartPath, (EventHandler)null!));

            Assert.AreEqual(1, gateway.Session.CloseMainWindowCount);
            Assert.AreEqual(1, gateway.Session.KillCount);
            Assert.IsFalse(gateway.Session.KeepRunning);
        });
    }

    /// <summary>準備済みBMIIDX windowの埋込みと再生開始をhostへ順に通知し、前景復旧も試行します。</summary>
    [TestMethod]
    public void BmiIdxEmbedsReadyWindowThroughWindowHost()
    {
        WithTemporaryPlayerFiles("BMIIDXView2015_64.exe", (root, executablePath, chartPath) =>
        {
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(17));
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                ForegroundWindow = new ExternalWindowHandle(new IntPtr(55)),
                SetForegroundWindowSucceeds = false
            };
            var player = new BMIIDXView2015(
                executablePath,
                new SettingsPlayerSettingsGateway(() => testSettings),
                gateway,
                waitPolicy: CreateWaitPolicy());
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            player.PlayStart(chartPath, (Action<object, EventArgs>)null!);

            Assert.AreEqual(
                "GetForegroundWindow,SetForegroundWindow,AttachBmiIdxWindow,NotifyBmiIdxPlaybackStarted,SetForegroundWindow",
                string.Join(",", windowHost.Operations));

            gateway.Session.KeepRunning = false;
            player.CloseProcess();
        });
    }

    /// <summary>停止不能BMIIDXは明示失敗してprocessを保持し、原因解消後の明示closeで同じprocessを回収します。</summary>
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
                new SettingsPlayerSettingsGateway(() => testSettings),
                gateway,
                CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);
            player.PlayStart(chartPath, (Action<object, EventArgs>)null!);
            gateway.Session.KillException = new InvalidOperationException("kill rejected");

            InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(
                player.CloseProcess);
            StringAssert.Contains(failure.Message, "could not be terminated");
            Assert.IsTrue(gateway.Session.KeepRunning);

            gateway.Session.KillException = null;
            player.CloseProcess();
            Assert.IsFalse(gateway.Session.KeepRunning);
            Assert.IsTrue(gateway.Session.KillCount >= 2);
        });
    }

    /// <summary>LR2のstyle・拡張style・保存配置を順にhostへ適用し、終了時に捕捉した配置を設定へ戻します。</summary>
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
                "<config><system><windowsize_x>800</windowsize_x><windowsize_y>600</windowsize_y><screenmode>1</screenmode></system><sound><volumemaster>100</volumemaster></sound><jukebox /></config>");
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
                new Lr2ConfigOwner(new LR2Config(configPath)),
                settingsGateway,
                gateway,
                waitPolicy: CreateWaitPolicy());
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            player.PlayStart(chartPath, (EventHandler)null!);

            CollectionAssert.Contains(windowHost.Operations, "ApplyLr2WindowStyle");
            CollectionAssert.Contains(windowHost.Operations, "ApplyLr2ExtendedWindowStyle");
            CollectionAssert.Contains(windowHost.Operations, "ApplyWindowPlacement");
            Assert.IsTrue(
                windowHost.Operations.IndexOf("ApplyLr2WindowStyle")
                < windowHost.Operations.IndexOf("ApplyLr2ExtendedWindowStyle"));
            Assert.IsTrue(
                windowHost.Operations.IndexOf("ApplyLr2ExtendedWindowStyle")
                < windowHost.Operations.IndexOf("ApplyWindowPlacement"));
            Assert.IsNotNull(windowHost.AppliedPlacement);

            gateway.Session.KeepRunning = false;
            player.CloseProcess();
            Assert.IsTrue(settingsGateway.PlacementUpdated);
            Assert.AreSame(windowHost.CapturedPlacement, settingsGateway.Placement);
            Assert.IsTrue(gateway.Session.HasExited);
            player.CloseProcess();
        });
    }

    /// <summary>style適用中のLR2終了を失敗として返し、後続配置適用を止めて試聴用設定を復元します。</summary>
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
                "<config><system><windowsize_x>800</windowsize_x><windowsize_y>600</windowsize_y><screenmode>1</screenmode></system><sound><volumemaster>100</volumemaster></sound><jukebox /></config>");
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
                new Lr2ConfigOwner(new LR2Config(configPath)),
                new RecordingPlayerSettingsGateway(),
                gateway,
                CreateWaitPolicy(TimeSpan.FromMilliseconds(100)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            Assert.ThrowsExactly<InvalidOperationException>(
                () => player.PlayStart(chartPath, (EventHandler)null!));
            CollectionAssert.Contains(windowHost.Operations, "ApplyLr2WindowStyle");
            CollectionAssert.DoesNotContain(windowHost.Operations, "ApplyWindowPlacement");
            var restoredDocument = XDocument.Load(configPath);
            Assert.AreEqual("1", ReadLr2Value(restoredDocument, "system", "screenmode"));
            Assert.AreEqual("100", ReadLr2Value(restoredDocument, "sound", "volumemaster"));
        });
    }

    /// <summary>試聴起動用XMLを一時公開しても設定draftは保存済み値を読み、起動後・終了後とも元の値と欠落要素を保持します。</summary>
    [TestMethod]
    public void Lr2PreviewRestoresSavedFieldsAndKeepsPlayerConfigInstanceUnchanged()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode><customfolder>保存済み</customfolder></system><sound><volumemaster>23</volumemaster></sound><jukebox /></config>");
            Lr2ConfigOwner playerConfig = new(new LR2Config(configPath));
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            gateway.Session.BeforeStart = () =>
            {
                var temporaryDocument = XDocument.Load(configPath);
                Assert.AreEqual("800", ReadLr2Value(temporaryDocument, "system", "windowsize_x"));
                Assert.AreEqual("600", ReadLr2Value(temporaryDocument, "system", "windowsize_y"));
                Assert.AreEqual("1", ReadLr2Value(temporaryDocument, "system", "screenmode"));
                Assert.AreEqual("100", ReadLr2Value(temporaryDocument, "sound", "volumemaster"));
                Assert.AreEqual("1", ReadLr2Value(temporaryDocument, "sound", "volumeflag"));

                Lr2ConfigSnapshot settingsDraft = playerConfig.Capture();
                Assert.AreEqual("640", ReadLr2Value(settingsDraft, "system", "windowsize_x"));
                Assert.AreEqual("480", ReadLr2Value(settingsDraft, "system", "windowsize_y"));
                Assert.AreEqual("0", ReadLr2Value(settingsDraft, "system", "screenmode"));
                Assert.AreEqual("23", ReadLr2Value(settingsDraft, "sound", "volumemaster"));
                Assert.AreEqual(23, settingsDraft.MasterVolume);
            };
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)));
            var player = new LR2body(
                executablePath,
                playerConfig,
                new RecordingPlayerSettingsGateway(),
                gateway,
                waitPolicy: CreateWaitPolicy());
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            player.PlayStart(chartPath, (EventHandler)null!);

            Assert.AreEqual("640", ReadLr2Value(playerConfig, "system", "windowsize_x"));
            Assert.AreEqual("480", ReadLr2Value(playerConfig, "system", "windowsize_y"));
            Assert.AreEqual("0", ReadLr2Value(playerConfig, "system", "screenmode"));
            Assert.AreEqual("23", ReadLr2Value(playerConfig, "sound", "volumemaster"));
            Assert.AreEqual(23, playerConfig.Capture().MasterVolume);
            var restoredDocument = XDocument.Load(configPath);
            Assert.AreEqual("保存済み", ReadLr2Value(restoredDocument, "system", "customfolder"));
            Assert.AreEqual("640", ReadLr2Value(restoredDocument, "system", "windowsize_x"));
            Assert.AreEqual("480", ReadLr2Value(restoredDocument, "system", "windowsize_y"));
            Assert.AreEqual("0", ReadLr2Value(restoredDocument, "system", "screenmode"));
            Assert.AreEqual("23", ReadLr2Value(restoredDocument, "sound", "volumemaster"));
            Assert.IsNull(restoredDocument.Element("config")?.Element("sound")?.Element("volumeflag"));

            gateway.Session.KeepRunning = false;
            gateway.Session.RaiseExited();

            var exitDocument = XDocument.Load(configPath);
            Assert.AreEqual("640", ReadLr2Value(exitDocument, "system", "windowsize_x"));
            Assert.AreEqual("480", ReadLr2Value(exitDocument, "system", "windowsize_y"));
            Assert.AreEqual("0", ReadLr2Value(exitDocument, "system", "screenmode"));
            Assert.AreEqual("23", ReadLr2Value(exitDocument, "sound", "volumemaster"));
            Assert.IsNull(exitDocument.Element("config")?.Element("sound")?.Element("volumeflag"));
        });
    }

    /// <summary>試聴開始時に捕捉した設定draftのルート編集を実保存し、player終了でその編集を古いXMLへ戻しません。</summary>
    [TestMethod]
    public void Lr2PreviewUsesTheSameDraftForRootSaveAfterStartupBoundary()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            Directory.CreateDirectory(Path.Combine(root, "RootA"));
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode></system><sound><volumemaster>23</volumemaster></sound><jukebox><path>RootA\\</path></jukebox></config>");
            Lr2ConfigOwner playerConfig = new(new LR2Config(configPath));
            Lr2ConfigSnapshot? settingsDraft = null;
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            gateway.Session.BeforeStart = () =>
            {
                settingsDraft = playerConfig.Capture();
                Assert.AreEqual("640", ReadLr2Value(settingsDraft, "system", "windowsize_x"));
                Assert.AreEqual("23", ReadLr2Value(settingsDraft, "sound", "volumemaster"));
            };
            var player = new LR2body(
                executablePath,
                playerConfig,
                new RecordingPlayerSettingsGateway(),
                gateway,
                waitPolicy: CreateWaitPolicy());
            ((IExternalWindowPlayer)player).AttachWindowHost(
                new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99))));

            player.PlayStart(chartPath, (EventHandler)null!);

            Assert.IsNotNull(settingsDraft);
            playerConfig.SaveRoots(new Lr2RootChange([Path.Combine(root, "RootA")], []));
            var savedAfterRootEdit = XDocument.Load(configPath);
            Assert.IsNull(savedAfterRootEdit.Element("config")?.Element("jukebox")?.Element("path"));
            Assert.AreEqual("640", ReadLr2Value(savedAfterRootEdit, "system", "windowsize_x"));
            Assert.AreEqual("23", ReadLr2Value(savedAfterRootEdit, "sound", "volumemaster"));

            gateway.Session.KeepRunning = false;
            gateway.Session.RaiseExited();
            var savedAfterExit = XDocument.Load(configPath);
            Assert.IsNull(savedAfterExit.Element("config")?.Element("jukebox")?.Element("path"));
            Assert.AreEqual("640", ReadLr2Value(savedAfterExit, "system", "windowsize_x"));
            Assert.AreEqual("23", ReadLr2Value(savedAfterExit, "sound", "volumemaster"));
        });
    }

    /// <summary>process開始前の失敗は元例外と元XMLを保持し、未起動processの終了確認・close・killを行わず次起動を許可します。</summary>
    [TestMethod]
    public void Lr2StartFailureBeforeProcessStartRestoresPublishedPreviewWithoutProcessCleanup()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode></system><sound><volumemaster>23</volumemaster><volumeflag>0</volumeflag></sound><jukebox /></config>");
            Lr2ConfigOwner playerConfig = new(new LR2Config(configPath));
            var startFailure = new InvalidOperationException("start rejected");
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.StartException = startFailure;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            var player = new LR2body(
                executablePath,
                playerConfig,
                new RecordingPlayerSettingsGateway(),
                gateway,
                waitPolicy: CreateWaitPolicy());
            ((IExternalWindowPlayer)player).AttachWindowHost(
                new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99))));

            InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(
                () => player.PlayStart(chartPath, (EventHandler)null!));

            Assert.AreSame(startFailure, failure);
            Assert.IsFalse(gateway.Session.Started);
            Assert.AreEqual(0, gateway.Session.HasExitedReadCount);
            Assert.AreEqual(0, gateway.Session.CloseMainWindowCount);
            Assert.AreEqual(0, gateway.Session.KillCount);
            var restoredDocument = XDocument.Load(configPath);
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

    /// <summary>window待機とkillの失敗を両方返し元XMLを復元し、残るprocessを原因解消後の明示closeで回収します。</summary>
    [TestMethod]
    public void Lr2MainWindowTimeoutRetainsUnkillableProcessForExplicitClose()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode></system><sound><volumemaster>23</volumemaster><volumeflag>0</volumeflag></sound><jukebox /></config>");
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.KillException = new InvalidOperationException("kill rejected");
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)));
            var player = new LR2body(
                executablePath,
                new Lr2ConfigOwner(new LR2Config(configPath)),
                new RecordingPlayerSettingsGateway(),
                gateway,
                CreateWaitPolicy(TimeSpan.FromMilliseconds(80)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(
                () => player.PlayStart(chartPath, (EventHandler)null!));

            StringAssert.Contains(failure.Message, configPath);
            var aggregate = failure.InnerException as AggregateException;
            Assert.IsNotNull(aggregate);
            Assert.AreEqual(2, aggregate!.InnerExceptions.Count);
            Assert.IsInstanceOfType<TimeoutException>(aggregate.InnerExceptions[0]);
            Assert.IsInstanceOfType<TimeoutException>(aggregate.InnerExceptions[1]);
            Assert.IsTrue(gateway.Session.Started);
            Assert.IsTrue(gateway.Session.KillCount > 0);
            Assert.IsTrue(gateway.Session.HasExitedReadCount > 0);
            var restoredDocument = XDocument.Load(configPath);
            Assert.AreEqual("640", ReadLr2Value(restoredDocument, "system", "windowsize_x"));
            Assert.AreEqual("23", ReadLr2Value(restoredDocument, "sound", "volumemaster"));

            gateway.Session.KillException = null;
            player.CloseProcess();

            Assert.IsFalse(gateway.Session.KeepRunning);
            Assert.AreEqual("640", ReadLr2Value(XDocument.Load(configPath), "system", "windowsize_x"));
        });
    }

    /// <summary>起動済みLR2のstyle待機だけが期限超過しても再生をkillせず、配置適用と元XML復元を継続します。</summary>
    [TestMethod]
    public void Lr2WindowStyleTimeoutKeepsEstablishedPlaybackRunning()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode></system><sound><volumemaster>23</volumemaster><volumeflag>0</volumeflag></sound><jukebox /></config>");
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                CompleteLr2WindowStyleApply = false
            };
            var player = new LR2body(
                executablePath,
                new Lr2ConfigOwner(new LR2Config(configPath)),
                new RecordingPlayerSettingsGateway(),
                gateway,
                CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            player.PlayStart(chartPath, (EventHandler)null!);

            CollectionAssert.Contains(windowHost.Operations, "ApplyLr2WindowStyle");
            CollectionAssert.DoesNotContain(windowHost.Operations, "ApplyLr2ExtendedWindowStyle");
            CollectionAssert.Contains(windowHost.Operations, "ApplyWindowPlacement");
            Assert.AreEqual(0, gateway.Session.KillCount);
            Assert.IsTrue(gateway.Session.KeepRunning);
            var restoredDocument = XDocument.Load(configPath);
            Assert.AreEqual("640", ReadLr2Value(restoredDocument, "system", "windowsize_x"));
            Assert.AreEqual("23", ReadLr2Value(restoredDocument, "sound", "volumemaster"));

            gateway.Session.KeepRunning = false;
            player.CloseProcess();
        });
    }

    /// <summary>起動後XML復元がlockで失敗しても元IOExceptionを返してprocessを保持し、lock解除後closeで復元と回収を完了します。</summary>
    [TestMethod]
    public void Lr2PreviewRestoreFailureAfterStartupKeepsRunningProcessForExplicitClose()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode></system><sound><volumemaster>23</volumemaster><volumeflag>0</volumeflag></sound><jukebox /></config>");
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            FileStream? lockedConfig = null;
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                Lr2WindowStyleApplyAttempt = () =>
                    lockedConfig = new FileStream(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            };
            var player = new LR2body(
                executablePath,
                new Lr2ConfigOwner(new LR2Config(configPath)),
                new RecordingPlayerSettingsGateway(),
                gateway,
                CreateWaitPolicy(TimeSpan.FromMilliseconds(80)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            try
            {
                Assert.ThrowsExactly<IOException>(
                    () => player.PlayStart(chartPath, (EventHandler)null!));

                Assert.AreEqual(0, gateway.Session.KillCount);
                Assert.IsTrue(gateway.Session.KeepRunning);
                CollectionAssert.Contains(windowHost.Operations, "ApplyLr2ExtendedWindowStyle");
            }
            finally
            {
                lockedConfig?.Dispose();
            }

            player.CloseProcess();

            Assert.IsFalse(gateway.Session.KeepRunning);
            var restoredDocument = XDocument.Load(configPath);
            Assert.AreEqual("640", ReadLr2Value(restoredDocument, "system", "windowsize_x"));
            Assert.AreEqual("23", ReadLr2Value(restoredDocument, "sound", "volumemaster"));
        });
    }

    /// <summary>起動側失敗とXML復元失敗が重なったら元の両例外と設定pathを保持し、後片付け失敗だけへ置換しません。</summary>
    [TestMethod]
    public void Lr2PreviewRestoreFailureKeepsPrimaryFailureAndConfigPath()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode></system><sound><volumemaster>23</volumemaster><volumeflag>0</volumeflag></sound><jukebox /></config>");
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            FileStream? lockedConfig = null;
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
                    new Lr2ConfigOwner(new LR2Config(configPath)),
                    new RecordingPlayerSettingsGateway(),
                    gateway,
                    CreateWaitPolicy(TimeSpan.FromMilliseconds(80)));
                ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

                InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(
                    () => player.PlayStart(chartPath, (EventHandler)null!));

                StringAssert.Contains(failure.Message, configPath);
                var aggregate = failure.InnerException as AggregateException;
                Assert.IsNotNull(aggregate);
                Assert.AreEqual(2, aggregate!.InnerExceptions.Count);
                Assert.IsInstanceOfType<InvalidOperationException>(aggregate.InnerExceptions[0]);
                Assert.IsInstanceOfType<IOException>(aggregate.InnerExceptions[1]);
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

    private static string ReadLr2Value(Lr2ConfigOwner owner, string section, string name) => ReadLr2Value(owner.Capture(), section, name);

    private static string ReadLr2Value(Lr2ConfigSnapshot snapshot, string section, string name) => name switch
    {
        "windowsize_x" => snapshot.WindowSizeX.ToString(),
        "windowsize_y" => snapshot.WindowSizeY.ToString(),
        "screenmode" => snapshot.ScreenModeWindow ? "1" : "0",
        "volumemaster" => snapshot.MasterVolume.ToString(),
        "volumeflag" => snapshot.VolumeEnabled ? "1" : "0",
        _ => throw new ArgumentException(name)
    };

    private static string? ReadLr2Value(XDocument document, string sectionName, string fieldName)
    {
        return document.Element("config")?.Element(sectionName)?.Element(fieldName)?.Value;
    }

    /// <summary>実INIの読取り・原子公開失敗を両viewer入口で観測し、旧内容と開始前の他processを保全します。原因解消後の明示開始だけを受け付けます。</summary>
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    public void ExternalViewerIniFailurePreservesBytesAndDoesNotReplaceProcess(bool ubmplay, int failureMode)
    {
        WithTemporaryPlayerFiles(ubmplay ? "uBMplay.exe" : "BMIIDXView2015_64.exe", (root, executable, chart) =>
        {
            string ini = Path.Combine(root, ubmplay ? "ubm.ini" : "BMIIDXView2015.ini");
            byte[] original = Encoding.GetEncoding("shift_jis").GetBytes(";日本語\r\n[Main]\r\nBMSVOLUME=12\r\n");
            if (failureMode == 2) { Directory.CreateDirectory(ini); }
            else { File.WriteAllBytes(ini, original); }
            var old = new RecordingExternalPlayerProcessSession { KeepRunning = true };
            old.Start();
            var gateway = new RecordingExternalPlayerProcessGateway { ExistingProcesses = [old] };
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(31));
            var host = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                EnumeratedWindows = [gateway.Session.MainWindowHandle],
                WindowClassName = "ThunderRT6FormDC",
                WindowTitle = chart
            };
            IBMSPlayer player = ubmplay
                ? new uBMplay(executable, new RecordingPlayerSettingsGateway(), gateway, CreateWaitPolicy(TimeSpan.FromMilliseconds(40)))
                : new BMIIDXView2015(executable, new RecordingPlayerSettingsGateway(), gateway, CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(host);
            try
            {
                using (FileStream? held = failureMode == 2 ? null : new FileStream(ini, FileMode.Open, FileAccess.Read,
                    failureMode == 0 ? FileShare.None : FileShare.Read))
                {
                    if (failureMode == 2) { Assert.ThrowsExactly<UnauthorizedAccessException>(() => player.PlayStart(chart).GetAwaiter().GetResult()); }
                    else { Assert.ThrowsExactly<IOException>(() => player.PlayStart(chart).GetAwaiter().GetResult()); }
                    Assert.AreEqual(0, old.CloseMainWindowCount);
                    Assert.AreEqual(0, old.KillCount);
                    Assert.AreEqual(0, gateway.PrepareCount);
                    Assert.IsFalse(gateway.Session.Started);
                    if (failureMode == 1) { CollectionAssert.AreEqual(original, File.ReadAllBytes(ini)); }
                }
                if (failureMode == 2) { Directory.Delete(ini); }
                else { CollectionAssert.AreEqual(original, File.ReadAllBytes(ini)); }
                player.PlayStart(chart).GetAwaiter().GetResult();
                Assert.IsTrue(gateway.Session.Started);
                Assert.AreEqual(1, old.CloseMainWindowCount);
            }
            finally { player.CloseProcess(); }
        });
    }

    /// <summary>初回INI不在時はBMIIDXが生成せず、uBMplayだけが従来の不足節を生成して起動します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExternalViewerMissingIniPreservesCreationContract(bool ubmplay)
    {
        WithTemporaryPlayerFiles(ubmplay ? "uBMplay.exe" : "BMIIDXView2015_64.exe", (root, executable, chart) =>
        {
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(31));
            var host = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                EnumeratedWindows = [gateway.Session.MainWindowHandle],
                WindowClassName = "ThunderRT6FormDC",
                WindowTitle = chart
            };
            IBMSPlayer player = ubmplay
                ? new uBMplay(executable, new RecordingPlayerSettingsGateway(), gateway, CreateWaitPolicy(TimeSpan.FromMilliseconds(40)))
                : new BMIIDXView2015(executable, new RecordingPlayerSettingsGateway(), gateway, CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(host);
            try
            {
                player.PlayStart(chart).GetAwaiter().GetResult();
                Assert.AreEqual(ubmplay, File.Exists(Path.Combine(root, ubmplay ? "ubm.ini" : "BMIIDXView2015.ini")));
            }
            finally { player.CloseProcess(); }
        });
    }

    /// <summary>自然終了の復元失敗を一度返してbackupを保持し、新規起動を阻止します。次の明示停止で元bytesへ復元できます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UbmplayRestoreFailureRetainsBackupAndBlocksNewStartUntilExplicitRetry(bool retryByStart)
    {
        WithTemporaryPlayerFiles("uBMplay.exe", (root, executable, chart) =>
        {
            string ini = Path.Combine(root, "ubm.ini");
            const string original = "[Main]\nAlwaysOnTop=True\nVSYNC=True\n[Option]\nBGA=1\nAutoSeparate=False\nSkinType=2\nVolume=12\n";
            File.WriteAllText(ini, original);
            byte[] originalBytes = File.ReadAllBytes(ini);
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(31));
            var host = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                EnumeratedWindows = [gateway.Session.MainWindowHandle],
                WindowClassName = "ThunderRT6FormDC",
                WindowTitle = chart
            };
            var player = new uBMplay(executable, new RecordingPlayerSettingsGateway(), gateway, CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(host);
            int notified = 0;
            EventArgs? exit = null;
            try
            {
                player.PlayStart(chart, (_, args) => { notified++; exit = args; }).GetAwaiter().GetResult();
                byte[] temporary = File.ReadAllBytes(ini);
                using (var held = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    gateway.Session.KeepRunning = false;
                    gateway.Session.RaiseExited();
                    gateway.Session.RaiseExited();
                    Assert.AreEqual(1, notified);
                    Assert.IsInstanceOfType<PlaybackExitEventArgs>(exit);
                    StringAssert.Contains(((PlaybackExitEventArgs)exit).Failure.ToString(), ini);
                    CollectionAssert.AreEqual(temporary, File.ReadAllBytes(ini));
                    Assert.ThrowsExactly<IOException>(() => player.PlayStart(chart).GetAwaiter().GetResult());
                    Assert.AreEqual(1, gateway.PrepareCount);
                }
                if (!retryByStart)
                {
                    player.CloseProcess();
                    CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(ini));
                }
                gateway.Session.KeepRunning = true;
                player.PlayStart(chart).GetAwaiter().GetResult();
                Assert.AreEqual(2, gateway.PrepareCount);
                player.CloseProcess();
                CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(ini));
            }
            finally { player.CloseProcess(); }
        });
    }

    /// <summary>mainの実終了を先に観測した明示開始は新mainを作り、解除前に捕捉された旧通知が届いても新window・callbackを保持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UbmplayExitedMainStartsReplacementBeforeDelayedOldNotification(bool synchronousNotification)
    {
        WithTemporaryPlayerFiles("uBMplay.exe", (root, executable, chart) =>
        {
            string ini = Path.Combine(root, "ubm.ini");
            File.WriteAllText(ini, "[Main]\nAlwaysOnTop=True\nVSYNC=True\n[Option]\nBGA=1\nAutoSeparate=False\nSkinType=2\nVolume=12\n");
            byte[] original = File.ReadAllBytes(ini);
            var oldMain = new RecordingExternalPlayerProcessSession { KeepRunning = true, MainWindowHandle = new ExternalWindowHandle(new IntPtr(31)) };
            var newMain = new RecordingExternalPlayerProcessSession { KeepRunning = true, MainWindowHandle = new ExternalWindowHandle(new IntPtr(32)) };
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.EnqueuePreparedSession(oldMain);
            gateway.EnqueuePreparedSession(newMain);
            var host = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                EnumeratedWindows = [oldMain.MainWindowHandle],
                WindowClassName = "ThunderRT6FormDC",
                WindowTitle = chart
            };
            var player = new uBMplay(executable, new RecordingPlayerSettingsGateway(), gateway, CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(host);
            int oldNotifications = 0;
            int newNotifications = 0;
            try
            {
                player.PlayStart(chart, (_, _) => oldNotifications++).GetAwaiter().GetResult();
                Action deliverOldExit = oldMain.CaptureExitedNotification();
                oldMain.KeepRunning = false;
                if (synchronousNotification) { oldMain.OnNextHasExited = oldMain.RaiseExited; }
                host.EnumeratedWindows = [newMain.MainWindowHandle];
                player.PlayStart(chart, (_, _) => newNotifications++).GetAwaiter().GetResult();
                Assert.IsTrue(newMain.Started);
                Assert.IsFalse(newMain.HasExited, "新しいmainを短命なrequestとして待ちません。");
                Assert.AreEqual(newMain.MainWindowHandle, host.LastAttachedUbmplayWindow);
                deliverOldExit();
                Assert.AreEqual(0, newNotifications);
                Assert.AreEqual(synchronousNotification ? 1 : 0, oldNotifications);
                Assert.AreEqual(newMain.MainWindowHandle, host.LastAttachedUbmplayWindow);
                newMain.KeepRunning = false;
                newMain.RaiseExited();
                Assert.AreEqual(1, newNotifications);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(ini));
            }
            finally { player.CloseProcess(); }
        });
    }

    /// <summary>main/requestのHasExited同期再入でも復元結果を一度だけロック外へ通知し、callbackから別threadの実Closeが進みます。復元失敗は同じ例外を返す開始・停止要求が通知を所有し、次の明示操作で再試行します。</summary>
    /// <remarks>requestはtimeoutとcleanup失敗でmainだけを終了した後、次の明示Closeが購読中のHasExitedを読む実経路です。返却例外と終了引数の同一原因・通知所有を確認し、Panelの一通知/一診断と状態保全は実uBMplay接続代表へ分担します。</remarks>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void UbmplaySynchronousExitNotifiesOutsideOutermostLock(bool requestExit, bool failRestore)
    {
        WithTemporaryPlayerFiles("uBMplay.exe", (root, executable, chart) =>
        {
            string ini = Path.Combine(root, "ubm.ini");
            File.WriteAllText(ini, "[Main]\nAlwaysOnTop=True\nVSYNC=True\n[Option]\nBGA=1\nAutoSeparate=False\nSkinType=2\nVolume=12\n");
            byte[] original = File.ReadAllBytes(ini);
            var main = new RecordingExternalPlayerProcessSession { KeepRunning = true, MainWindowHandle = new ExternalWindowHandle(new IntPtr(31)) };
            var request = new RecordingExternalPlayerProcessSession { KeepRunning = true, KillException = new InvalidOperationException("request kill rejected") };
            var failedNewMain = new RecordingExternalPlayerProcessSession { KeepRunning = true, MainWindowHandle = new ExternalWindowHandle(new IntPtr(32)) };
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.EnqueuePreparedSession(main);
            gateway.EnqueuePreparedSession(requestExit ? request : failedNewMain);
            var host = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                EnumeratedWindows = [main.MainWindowHandle],
                WindowClassName = "ThunderRT6FormDC",
                WindowTitle = chart
            };
            failedNewMain.BeforeStart = () => host.WindowTitle = "different chart";
            var player = new uBMplay(executable, new RecordingPlayerSettingsGateway(), gateway, CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(host);
            using var consumerReady = new ManualResetEventSlim();
            using var runConsumer = new ManualResetEventSlim();
            using var consumerCompleted = new ManualResetEventSlim();
            using var consumerAbort = new CancellationTokenSource();
            int notifications = 0;
            bool restoredAtCallback = false;
            bool transientPreservedAtCallback = false;
            bool closeCompletedInsideCallback = false;
            Exception? callbackFailure = null;
            bool failureNotificationOwnedByCaller = false;
            Exception? consumerFailure = null;
            Exception? producerFailure = null;
            byte[] transient = [];
            Action<object, EventArgs> callback = (_, args) =>
            {
                notifications++;
                callbackFailure = (args as PlaybackExitEventArgs)?.Failure;
                failureNotificationOwnedByCaller = (args as PlaybackExitEventArgs)?.FailureNotificationOwnedByCaller == true;
                byte[] current = File.ReadAllBytes(ini);
                restoredAtCallback = original.SequenceEqual(current);
                transientPreservedAtCallback = transient.SequenceEqual(current);
                runConsumer.Set();
                closeCompletedInsideCallback = consumerCompleted.Wait(TimeSpan.FromSeconds(2));
            };
            FileStream? heldIni = null;
            Task consumer = Task.CompletedTask;
            Task producer = Task.CompletedTask;
            try
            {
                player.PlayStart(chart, requestExit ? null! : callback).GetAwaiter().GetResult();
                if (requestExit)
                {
                    Assert.ThrowsExactly<AggregateException>(() => player.PlayStart(chart, callback).GetAwaiter().GetResult());
                    Assert.IsTrue(main.HasExited);
                    Assert.IsFalse(request.HasExited);
                }
                RecordingExternalPlayerProcessSession exiting = requestExit ? request : main;
                Action deliverExit = exiting.CaptureExitedNotification();
                exiting.KeepRunning = false;
                exiting.OnNextHasExited = deliverExit;
                transient = File.ReadAllBytes(ini);
                if (failRestore) { heldIni = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.Read); }
                consumer = Task.Run(() =>
                {
                    consumerReady.Set();
                    runConsumer.Wait();
                    try
                    {
                        if (!consumerAbort.IsCancellationRequested) { player.CloseProcess(); }
                    }
                    catch (Exception failure) { consumerFailure = failure; }
                    finally { consumerCompleted.Set(); }
                });
                Assert.IsTrue(consumerReady.Wait(TimeSpan.FromSeconds(5)));
                producer = Task.Run(() =>
                {
                    try
                    {
                        if (requestExit) { player.CloseProcess(); }
                        else { player.PlayStart(chart, null!).GetAwaiter().GetResult(); }
                    }
                    catch (Exception failure) { producerFailure = failure; }
                });
                producer.GetAwaiter().GetResult();
                runConsumer.Set();
                consumer.GetAwaiter().GetResult();
                Assert.AreEqual(1, notifications);
                Assert.AreEqual(failRestore, failureNotificationOwnedByCaller, "同じ復元失敗を返す最外の開始・停止要求が失敗通知を所有します。");
                Assert.IsTrue(closeCompletedInsideCallback, "callbackからの別thread停止が外側lockのために止まりません。");
                Assert.AreEqual(!failRestore, restoredAtCallback);
                Assert.AreEqual(failRestore, transientPreservedAtCallback);
                if (failRestore)
                {
                    Assert.IsNotNull(callbackFailure);
                    Assert.IsTrue(callbackFailure.ToString().Contains(ini, StringComparison.Ordinal));
                    Assert.IsTrue(ContainsCause(producerFailure, callbackFailure), "通知責務の包装後も同一backupの元復元失敗を原因として保持します。");
                    Assert.IsNotNull(consumerFailure, "別要求の復元失敗もlock取得後の実終端として扱います。");
                    Assert.AreEqual(requestExit ? 2 : 1, gateway.PrepareCount, "復元失敗後に新mainを準備しません。");
                }
                else
                {
                    Assert.IsNull(callbackFailure);
                    Assert.IsNull(consumerFailure);
                    if (requestExit) { Assert.IsNull(producerFailure); }
                    else { Assert.IsNotNull(producerFailure, "新backupの起動後故障をcleanupしてから旧通知を配送します。"); }
                }
                deliverExit();
                Assert.AreEqual(1, notifications, "購読解除前snapshotの再配送も通知を重ねません。");
            }
            finally
            {
                consumerAbort.Cancel();
                runConsumer.Set();
                try { producer.GetAwaiter().GetResult(); consumer.GetAwaiter().GetResult(); }
                finally { heldIni?.Dispose(); player.CloseProcess(); }
            }
            CollectionAssert.AreEqual(original, File.ReadAllBytes(ini));
        });

        static bool ContainsCause(Exception? failure, Exception cause) => failure != null
            && (ReferenceEquals(failure, cause)
                || (failure is AggregateException aggregate
                    ? aggregate.InnerExceptions.Any(inner => ContainsCause(inner, cause))
                    : ContainsCause(failure.InnerException, cause)));
    }

    /// <summary>旧main終了と新main同期終了が同じ要求で確定しても異なるcallbackを順番に配送し、先の通知故障で後の通知を欠落させません。</summary>
    [TestMethod]
    public void UbmplaySynchronousExitsKeepEverySnapshotWhenEarlierNotificationFails()
    {
        WithTemporaryPlayerFiles("uBMplay.exe", (root, executable, chart) =>
        {
            string ini = Path.Combine(root, "ubm.ini");
            File.WriteAllText(ini, "[Main]\nAlwaysOnTop=True\nVSYNC=True\n[Option]\nBGA=1\nAutoSeparate=False\nSkinType=2\nVolume=12\n");
            byte[] original = File.ReadAllBytes(ini);
            var oldMain = new RecordingExternalPlayerProcessSession { KeepRunning = true };
            var newMain = new RecordingExternalPlayerProcessSession();
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.EnqueuePreparedSession(oldMain);
            gateway.EnqueuePreparedSession(newMain);
            var host = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                EnumeratedWindows = [new ExternalWindowHandle(new IntPtr(31))],
                WindowClassName = "ThunderRT6FormDC",
                WindowTitle = chart
            };
            var player = new uBMplay(executable, new RecordingPlayerSettingsGateway(), gateway, CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(host);
            var notifications = new List<int>();
            try
            {
                player.PlayStart(chart, (_, _) => { notifications.Add(1); throw new IOException("first callback failed"); }).GetAwaiter().GetResult();
                Action oldExit = oldMain.CaptureExitedNotification();
                oldMain.KeepRunning = false;
                oldMain.OnNextHasExited = oldExit;
                newMain.BeforeStart = () =>
                {
                    host.WindowClassName = "no viewer window";
                    newMain.OnNextHasExited = newMain.CaptureExitedNotification();
                };
                Exception? startupFailure = null;
                try { player.PlayStart(chart, (_, _) => notifications.Add(2)).GetAwaiter().GetResult(); }
                catch (Exception failure) { startupFailure = failure; }
                Assert.IsNotNull(startupFailure);
                CollectionAssert.AreEqual(new[] { 1, 2 }, notifications);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(ini));
                oldExit();
                CollectionAssert.AreEqual(new[] { 1, 2 }, notifications);
            }
            finally { player.CloseProcess(); }
        });
    }

    /// <summary>既存mainを再利用した要求でも、自然終了は最後に受け付けた要求のcallbackへ一回届きます。</summary>
    [TestMethod]
    public void UbmplayReusedProcessNotifiesCurrentRequestCallback()
    {
        WithTemporaryPlayerFiles("uBMplay.exe", (root, executable, chart) =>
        {
            var gateway = new RecordingExternalPlayerProcessGateway();
            var main = new RecordingExternalPlayerProcessSession { KeepRunning = true, MainWindowHandle = new ExternalWindowHandle(new IntPtr(31)) };
            gateway.EnqueuePreparedSession(main);
            gateway.EnqueuePreparedSession(new RecordingExternalPlayerProcessSession());
            var host = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                EnumeratedWindows = [main.MainWindowHandle],
                WindowClassName = "ThunderRT6FormDC",
                WindowTitle = chart
            };
            var player = new uBMplay(executable, new RecordingPlayerSettingsGateway(), gateway, CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(host);
            int old = 0;
            int current = 0;
            try
            {
                player.PlayStart(chart, (_, _) => old++).GetAwaiter().GetResult();
                player.PlayStart(chart, (_, _) => current++).GetAwaiter().GetResult();
                main.KeepRunning = false;
                main.RaiseExited();
                main.RaiseExited();
                Assert.AreEqual(0, old);
                Assert.AreEqual(1, current);
            }
            finally { player.CloseProcess(); }
        });
    }

    /// <summary>uBMplayの探索・起動・埋込みと一時停止/早送りのkey入力をhostへ渡し、既存windowの終了時にはdetachします。</summary>
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
                WindowTitle = chartPath,
                ForegroundWindow = new ExternalWindowHandle(new IntPtr(55)),
                SetForegroundWindowSucceeds = false
            };
            var player = new uBMplay(
                executablePath,
                new SettingsPlayerSettingsGateway(() => testSettings),
                gateway,
                waitPolicy: CreateWaitPolicy());
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
            CollectionAssert.AreEqual(new[]
            {
                "11:False:True", "11:False:False",
                "11:False:True", "11:False:False",
                "80:True:True", "80:True:False"
            }, windowHost.KeyEvents);

            windowHost.LegacyWindowEmbedding = true;
            gateway.ExistingProcesses = new[] { gateway.Session };
            // 終了済mainのhandleは終了確認時に破棄するため、生存mainの再利用で既存のdetach契約を確認します。
            gateway.EnqueuePreparedSession(new RecordingExternalPlayerProcessSession());
            player.PlayStart(chartPath, (Action<object, EventArgs>)null!).GetAwaiter().GetResult();
            CollectionAssert.Contains(windowHost.Operations, "DetachUbmplayWindow");
            player.CloseProcess();
        });
    }

    /// <summary>uBMplayの一時INI変更を終了通知・明示close・起動失敗それぞれで元のバイト列へ復元します。</summary>
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
                new SettingsPlayerSettingsGateway(() => testSettings),
                gateway,
                waitPolicy: CreateWaitPolicy());
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

            Assert.ThrowsExactly<InvalidOperationException>(
                () => player.PlayStart(chartPath, (Action<object, EventArgs>)null!));
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(iniPath));
        });
    }

    /// <summary>kill失敗後もINI復元権と終了callbackを保持し、遅い書込み後の自然終了で元bytesへ復元して一回通知します。</summary>
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
                new SettingsPlayerSettingsGateway(() => testSettings),
                gateway,
                CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);
            int exits = 0;
            player.PlayStart(chartPath, (_, _) => exits++);
            gateway.Session.KillException = new InvalidOperationException("kill rejected");

            Assert.ThrowsExactly<InvalidOperationException>(player.CloseProcess);

            File.WriteAllText(iniPath, "late process write", Encoding.GetEncoding("shift_jis"));
            gateway.Session.KeepRunning = false;
            gateway.Session.RaiseExited();
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(iniPath));
            Assert.AreEqual(1, exits, "終了失敗で後続の自然終了callbackを失いません。");
        });
    }

    /// <summary>追加要求processの停止失敗も所有し続け、遅いINI書込み後の終了通知で保存済み値を復元します。</summary>
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
                new SettingsPlayerSettingsGateway(() => testSettings),
                gateway,
                CreateWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);
            player.PlayStart(chartPath, (Action<object, EventArgs>)null!);

            Assert.ThrowsExactly<AggregateException>(
                () => player.PlayStart(chartPath, (Action<object, EventArgs>)null!));
            Assert.IsTrue(requestSession.KillCount >= 1);

            Assert.IsTrue(mainSession.HasExited);
            Assert.IsFalse(requestSession.HasExited);
            Assert.AreNotEqual(original, File.ReadAllText(iniPath, Encoding.GetEncoding("shift_jis")), "main終了後もrequest生存中は復元しません。");
            File.WriteAllText(iniPath, "late request write", Encoding.GetEncoding("shift_jis"));
            requestSession.KeepRunning = false;
            requestSession.RaiseExited();
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(iniPath));
        });
    }

    /// <summary>既存uBMplayの置換時に試聴用INIを維持し、新しい所有processのcloseで元のバイト列へ復元します。</summary>
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
                new SettingsPlayerSettingsGateway(() => testSettings),
                gateway,
                waitPolicy: CreateWaitPolicy());
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            player.PlayStart(chartPath, (Action<object, EventArgs>)null!);
            StringAssert.Contains(File.ReadAllText(iniPath, Encoding.GetEncoding("shift_jis")), "AlwaysOnTop=False");

            gateway.Session.KeepRunning = false;
            player.CloseProcess();
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(iniPath));
        });
    }

    /// <summary>保存済みの最大化配置は通常windowとして指定サイズへ復元し、過去のflagsや最大化状態を持ち越しません。</summary>
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
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        Directory.CreateDirectory(root);
        string executablePath = Path.Combine(root, executableName);
        string chartPath = Path.Combine(root, "chart.bms");
        File.WriteAllText(executablePath, string.Empty);
        File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n");
        {
            action(root, executablePath, chartPath);
        }

    }

    /// <summary>fake processの状態は各ケースが明示し、待機だけでprocessを終了させず期限を進めます。</summary>
    private static ExternalPlayerWaitPolicy CreateWaitPolicy(TimeSpan? timeout = null)
    {
        var clock = new ManualExternalPlayerTime();
        return new ExternalPlayerWaitPolicy(timeout ?? TimeSpan.FromSeconds(10), clock, clock.Wait);
    }

    /// <summary>一つのpolicy専用の単調時計です。同期waitが要求した時間だけ進みます。</summary>
    private sealed class ManualExternalPlayerTime : TimeProvider
    {
        internal TimeSpan Elapsed { get; private set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Elapsed.Ticks;

        internal void Wait(TimeSpan interval) => Elapsed += interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(1);
    }
}
