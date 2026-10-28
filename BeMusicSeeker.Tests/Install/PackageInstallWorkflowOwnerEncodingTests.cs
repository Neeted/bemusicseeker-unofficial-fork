using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>単体取込みで実指定したBig5を同じ親の再投入後の新entryへ引き継ぎ、実推定・自動後段から成功先の所持DBへ保存します。</summary>
    /// <remarks>初段と後段は別sessionです。置換時の失敗保全とDST非継承は既存scope系列へ分担し、この代表は固定bytes/titleと導入後の永続値を照合します。</remarks>
    [TestMethod]
    public async Task AutomaticContinuation_ReimportedPendingFixedEncodingReachesInstalledDatabase()
    {
        const string known = "#TITLE 中文\r\n#ARTIST artist\r\n#BPM 120\r\n#WAV01 tone.wav\r\n#00111:01\r\n";
        await using var fixture = new AutomaticContinuationFixture(false, false,
            destinationSounds: ["tone.wav"], installedChartBodies: [known]);
        File.WriteAllText(Path.Combine(fixture.Destination, "owned-0000.bms"), known, new UTF8Encoding(true));
        string source = Path.Combine(fixture.Root, "Incoming");
        string path = InstallEncodingTestInput.Write(source);
        string companionPath = Path.Combine(source, "companion.bms");
        File.WriteAllText(companionPath, known + "#PLAYLEVEL 12\r\n", new UTF8Encoding(true));
        await fixture.InitializeAsync();
        Assert.AreEqual("中文", fixture.Library.BmsCharts.Single().RawTitle);
        Assert.IsTrue(fixture.Owner.Enqueue([path]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        ChartPackage pending = fixture.Library.ChartPackagesPending.Single();
        Assert.AreEqual(path, pending.path);
        Assert.AreEqual(1, pending.ChartEntries.Count);
        PackageChartEntry original = pending.ChartEntries.Single();
        Assert.AreEqual(path, original.Chart.Path);
        Assert.IsFalse(fixture.Library.BmsCharts.Any(chart => chart.Path == companionPath));
        Assert.IsFalse(fixture.Library.ChartPackagesInstalled.SelectMany(package => package.ChartEntries)
            .Any(entry => entry.Chart.Path == companionPath));
        using (LR2SongDBExtended firstReadback = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            Assert.AreEqual(path, firstReadback.Table<LR2SongDBExtended.install>().Single().path);
            Assert.IsNull(firstReadback.Find<LR2SongDB.song>(companionPath));
        }
        fixture.Library.SetBMSFilesEncoding([original.Chart], "big5", pendingEntries: [original]);
        InstallEncodingTestInput.AssertFixed(original.Chart, path);
        fixture.Options = fixture.CreateOptions(true, false);
        Assert.IsTrue(fixture.Owner.Enqueue([source]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.IsNotNull(fixture.Result);
        Assert.IsNotNull(fixture.Result.EstimatedInstallReceipt);
        Assert.IsTrue(fixture.Result.EstimatedInstallReceipt.DurableCommit);
        Assert.IsFalse(fixture.Result.EstimatedInstallReceipt.HasRequiredFailure);
        Assert.AreEqual(1, fixture.Result.EstimatedInstallReceipt.ApplyCounts.InstalledTargetApplyCount);
        Assert.AreEqual(1, fixture.Result.EstimatedInstallReceipt.ApplyCounts.RequiredPublicationCount);
        Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
        string installedPath = Path.Combine(fixture.Destination, "chart.bms");
        PackageChartEntry installed = fixture.Library.ChartPackagesInstalled.SelectMany(package => package.ChartEntries)
            .Single(entry => entry.Chart.Path == installedPath);
        Assert.AreNotSame(original, installed);
        InstallEncodingTestInput.AssertFixed(original.Chart, path);
        InstallEncodingTestInput.AssertFixed(installed.Chart, installedPath);
        InstallEncodingTestInput.AssertFixed(fixture.Library.BmsCharts.Single(chart => chart.Path == installedPath), installedPath);
        CollectionAssert.AreEqual(InstallEncodingTestInput.Bytes, File.ReadAllBytes(installedPath));
        Assert.AreEqual(100, installed.Chart.WAVHealth);
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        Assert.AreEqual("中文", db.Find<LR2SongDB.song>(installedPath).title);
        Assert.AreEqual(InstallEncodingTestInput.Md5, db.Find<LR2SongDB.song>(installedPath).hash);
        LR2SongDBExtended.maintenance saved = db.Find<LR2SongDBExtended.maintenance>(installedPath);
        Assert.AreEqual("big5", saved.encoding);
        Assert.IsTrue(saved.is_encoding_fixed);
        Assert.AreEqual(1, saved.wav_files_defined);
        Assert.AreEqual(1, saved.wav_files_existing);
        Assert.IsNull(db.Find<LR2SongDBExtended.maintenance>(path));
    }
}
