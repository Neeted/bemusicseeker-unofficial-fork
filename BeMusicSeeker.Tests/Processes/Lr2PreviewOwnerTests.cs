using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class ExternalPlayerProcessGatewayTests
{
    /// <summary>process開始境界で別workerの通常保存が完了し、overlay中は実ファイルだけに試聴値を保ち、初回復元後の後続登録も終了で維持します。</summary>
    [TestMethod]
    public void Lr2PreviewNormalSaveCompletesOnAnotherWorkerAndOverlayEndsAfterStartup()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executable, chart) =>
        {
            string path = CreateLr2Config(root, "<config><system><windowsize_x>640</windowsize_x></system><jukebox /></config>");
            Lr2ConfigOwner owner = new(new LR2Config(path));
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            Task save = Task.CompletedTask;
            gateway.Session.BeforeStart = () =>
            {
                save = Task.Run(() => owner.SaveRoots(new Lr2RootChange([], [Path.Combine(root, "B")])));
                Assert.IsTrue(save.Wait(TimeSpan.FromSeconds(2)), "process待機の間もXML所有者の保存排他を解放します。");
                save.GetAwaiter().GetResult();
                Assert.AreEqual("800", ReadLr2Value(XDocument.Load(path), "system", "windowsize_x"));
                Assert.AreEqual(640, owner.Capture().WindowSizeX);
            };
            var player = new LR2body(executable, owner, new RecordingPlayerSettingsGateway(), gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99))));
            try
            {
                player.PlayStart(chart, (EventHandler?)null);
                owner.SaveRoots(new Lr2RootChange([], [Path.Combine(root, "C")]));
                Assert.AreEqual("640", ReadLr2Value(XDocument.Load(path), "system", "windowsize_x"));
                gateway.Session.KeepRunning = false;
                gateway.Session.RaiseExited();
                CollectionAssert.AreEquivalent(new[] { "B", "C" }, new LR2Config(path).GetBMSSearchDirectoriesForChangeTracking().Select(Path.GetFileName).ToArray());
                Assert.IsNull(XDocument.Load(path).Element("config")?.Element("sound"));
            }
            finally
            {
                try { save.GetAwaiter().GetResult(); }
                finally { gateway.Session.KeepRunning = false; player.CloseProcess(); }
            }
        });
    }

    /// <summary>processが既に終了した後の復元失敗もscopeを保持し、新開始を阻止した上で明示Closeか次開始の復元成功から再開できます。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Lr2PreviewRestoreFailureAfterProcessExitRetainsScopeForExplicitRetry(bool retryOnStart)
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executable, chart) =>
        {
            string path = CreateLr2Config(root, "<config><system><windowsize_x>640</windowsize_x></system><jukebox /></config>");
            Lr2ConfigOwner owner = new(new LR2Config(path));
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            var player = new LR2body(executable, owner, new RecordingPlayerSettingsGateway(), gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99))));
            try
            {
                player.PlayStart(chart, (EventHandler?)null);
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    gateway.Session.KeepRunning = false;
                    InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(gateway.Session.RaiseExited);
                    StringAssert.Contains(failure.Message, path);
                    Assert.ThrowsException<InvalidOperationException>(() => owner.BeginPreview());
                    Assert.ThrowsException<InvalidOperationException>(() => player.PlayStart(chart, (EventHandler?)null));
                }
                if (!retryOnStart) { player.CloseProcess(); }
                gateway.Session.KeepRunning = true;
                player.PlayStart(chart, (EventHandler?)null);
                Assert.AreEqual("640", ReadLr2Value(XDocument.Load(path), "system", "windowsize_x"));
            }
            finally { gateway.Session.KeepRunning = false; player.CloseProcess(); }
        });
    }
    /// <summary>試聴XMLの初回公開に失敗した場合は未起動processへ終了処理を行わず、不要な復元失敗で元IOExceptionを置換せず次の明示開始を許可します。</summary>
    [TestMethod]
    public void Lr2PreviewInitialWriteFailureEndsUnpublishedScopeAndKeepsOriginalCause()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executable, chart) =>
        {
            string path = CreateLr2Config(root, "<config><jukebox /></config>");
            byte[] before = File.ReadAllBytes(path);
            Lr2ConfigOwner owner = new(new LR2Config(path));
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            var player = new LR2body(executable, owner, new RecordingPlayerSettingsGateway(), gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99))));
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                { Assert.ThrowsException<IOException>(() => player.PlayStart(chart, (EventHandler?)null)); }
                CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
                Assert.IsFalse(gateway.Session.Started);
                Assert.AreEqual(0, gateway.Session.HasExitedReadCount);
                Assert.AreEqual(0, gateway.Session.KillCount);
                player.PlayStart(chart, (EventHandler?)null);
                Assert.IsTrue(gateway.Session.Started);
            }
            finally { gateway.Session.KeepRunning = false; player.CloseProcess(); }
        });
    }

}
