using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class ApplicationCompositionTests
{
    /// <summary>単独モードのLR2試聴とプレイヤー再生成も、外部変更を取り込まず構成内で成立した登録を実XMLへ出力します。</summary>
    [TestMethod]
    public void StandaloneLr2PlayerRecreationUsesTheAdoptedConfiguration()
    {
        string root = Path.Combine(Path.GetTempPath(), "BmsLr2Composition", Guid.NewGuid().ToString("N"));
        string configDirectory = Path.Combine(root, "LR2files", "Config");
        Directory.CreateDirectory(configDirectory);
        string path = Path.Combine(configDirectory, "config.xml");
        string executable = Path.Combine(root, "LR2body.exe");
        File.WriteAllBytes(executable, []);
        File.WriteAllText(path, "<config><player><id>original</id></player><jukebox><path>A</path></jukebox></config>");
        var gateway = new ExternalPlayerProcessGatewayTests.RecordingExternalPlayerProcessGateway();
        gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
        var composition = new ApplicationComposition(settingsEditSession: new FakeSettingsEditSession { Values = testSettings },
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), externalPlayerProcessGateway: gateway,
            applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        var settings = new StartupSettingsSnapshot { OperationModeLR2DB = false, UsePlayerLR2body = true, LR2bodyPath = executable, LR2ConfigXmlPath = path };
        try
        {
            Lr2ConfigOwner owner = composition.ResolveLr2Config(path);
            owner.SaveRoots(new Lr2RootChange([], [Path.Combine(root, "B")]));
            foreach (int iteration in new[] { 1, 2 })
            {
                File.WriteAllText(path, "<external-broken");
                gateway.Session.KeepRunning = true;
                IBMSPlayer player = composition.CreateBmsPlayerForSettings(settings);
                ((IExternalWindowPlayer)player).AttachWindowHost(new ExternalPlayerProcessGatewayTests.RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99))));
                try
                {
                    player.PlayStart(Path.Combine(root, "test.bms"));
                    CollectionAssert.AreEquivalent(new[] { "A", "B" }, new LR2Config(path).GetBMSSearchDirectoriesForChangeTracking().Select(Path.GetFileName).ToArray());
                    Assert.AreEqual("original", XDocument.Load(path).Element("config")?.Element("player")?.Element("id")?.Value);
                }
                finally { gateway.Session.KeepRunning = false; player.CloseProcess(); }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
